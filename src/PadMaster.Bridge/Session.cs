using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace PadMaster;

public sealed record TurnState(int Generation, bool Busy, int Queued, bool CanRetry, bool UsedWrites, bool Restarting, string Notice, long Stamp);
public sealed record SendReceipt(long Ticket, bool Queued);

public sealed class Session(string cli, string cwd, string mcpConfig, Action<string> logSink) : IDisposable
{
    readonly Action<string> log = text => { try { logSink(text); } catch { } };
    sealed record Message(long Ticket, string Text, bool Hidden) { public int Generation { get; set; } }
    public string Kind { get; init; } = "claude-code";
    public event Action<string>? Reply;
    public event Action<string>? Activity;
    public event Action<string>? Failure;
    public event Action<bool>? Done;
    public event Action? HiddenTurn;
    public event Action? Interrupted;
    public event Action<TurnState>? StateChanged;
    public event Action<long, int, bool>? TurnStarted;
    public string? LastSessionId { get; private set; }
    public int Starts { get; private set; }
    readonly CancellationTokenSource _stop = new();
    readonly List<string> _order = [];
    readonly Dictionary<string, string> _messages = [];
    readonly HashSet<string> _tools = [];
    readonly List<Message> _pending = [];
    readonly List<Action> _raise = [];
    readonly Dictionary<string, int> _kinds = [];
    readonly object _gate = new();
    readonly Channel<bool> _wake = Channel.CreateUnbounded<bool>();
    Process? _child;
    Task? _worker;
    Message? _current, _retryMessage;
    string _message = "", _lastUserText = "", _notice = "";
    int _anonymous, _generation, _initializations, _attemptInitializations, _sentGeneration, _stderrLines, _activeTools, _lines, _stalls, _firstLineMs;
    long _ticket;
    static long _stamps;
    bool _attemptActive, _turn, _reportedFailure, _timedOut, _resumedAttempt, _attemptErrorResult, _queuePaused, _canRetry, _usedWrites, _held, _restarting, _exitNotified, _countsLogged, _codexCompleted;
    DateTime _lastEvent, _turnStart;
    Timer? _deadline;
    public string LastReply { get; private set; } = "";
    public int ReplyRevision { get; private set; }
    public int Lines { get { lock (_gate) return _lines; } }
    public int Initializations { get { lock (_gate) return _initializations; } }
    public int ParsedMessages { get { lock (_gate) return _messages.Count; } }
    public bool Paused { get { lock (_gate) return _queuePaused; } }
    public int Generation { get { lock (_gate) return _generation; } }
    public TimeSpan TurnTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan SilenceTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromSeconds(1);
    public Func<DateTime> Now { get; init; } = () => DateTime.UtcNow;
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
    public Action<Process> Launch { get; init; } = process => process.Start();
    public bool Busy { get { lock (_gate) return _turn && !_held; } }
    public TurnState State { get { lock (_gate) return Snapshot(); } }
    TurnState Snapshot() => new(_generation, _turn && !_held, _pending.Count, _canRetry, _usedWrites, _restarting, _notice, Interlocked.Increment(ref _stamps));
    void Publish() { var state = Snapshot(); Queue(() => StateChanged?.Invoke(state)); }
    void Queue(Action raise) => _raise.Add(raise);
    void Fire()
    {
        List<Action> pending;
        lock (_gate) { if (_raise.Count == 0) return; pending = [.. _raise]; _raise.Clear(); }
        foreach (var raise in pending) try { raise(); } catch (Exception e) { log($"assistant event: {SafeDiagnostics.Error(e)}"); }
    }
    static string Word(string? value) => value is { Length: > 0 } text && text.Length <= 24 && text.All(c => char.IsAsciiLetterLower(c) || c == '_') ? text : "other";
    void Count(string? kind) { var word = Word(kind); _kinds[word] = _kinds.GetValueOrDefault(word) + 1; }
    void LogCounts(string outcome)
    {
        if (_countsLogged || _lines == 0) return;
        _countsLogged = true;
        log($"turn lines: generation={_generation}, outcome={outcome}, total={_lines}, first={_firstLineMs} ms, " + string.Join(" ", _kinds.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value)));
    }
    public Task Completion { get { lock (_gate) return _worker ?? Task.CompletedTask; } }
    public static IReadOnlyList<string> Arguments(string config, string? resume) => new[] { "-p", "--verbose", "--input-format", "stream-json", "--output-format", "stream-json", "--include-partial-messages", "--permission-mode", "auto", "--tools", "Read,Grep,Glob", "--allowedTools", "Read,Grep,Glob,mcp__launchbox__*", "--setting-sources", "project", "--mcp-config", config, "--strict-mcp-config" }.Concat(resume == null ? [] : new[] { "--resume", resume }).Concat(new[] { "--disallowedTools" }).Concat(DeniedTools).ToArray();
    public static IReadOnlyList<string> DeniedTools { get; } = new[] { ".claude", ".ssh", ".aws", ".azure", ".gnupg" }.SelectMany(folder => new[] { "Read", "Grep", "Glob" }.Select(tool => $"{tool}(~/{folder}/**)" )).ToArray();
    public static IReadOnlyList<string> AuthArguments { get; } = ["--setting-sources", "project", "auth", "status", "--json"];
    public static IReadOnlyList<string> CodexArguments(string url, string? resume) => new[] { "exec" }.Concat(resume == null ? [] : new[] { "resume", resume })
        .Concat(new[] { "--json", "--skip-git-repo-check", "--ignore-user-config", "--ignore-rules" }).Concat(resume == null ? new[] { "-s", "read-only" } : [])
        .Concat(new[] { "-c", "approval_policy=never", "-c", "features.shell_tool=false", "-c", "mcp_servers.launchbox.url=" + url, "-c", "mcp_servers.launchbox.bearer_token_env_var=PADMASTER_MCP_TOKEN", "-c", "mcp_servers.launchbox.default_tools_approval_mode=approve" }).ToArray();
    public static IReadOnlyList<string> StatusArguments(string kind) => kind == "codex" ? ["login", "status"] : AuthArguments;
    public static ProcessStartInfo ChatCommand(string cli, string cwd, string config, string? resume, string kind = "claude-code")
    {
        if (kind == "claude-code") return Command(cli, cwd, Arguments(config, resume));
        if (kind != "codex") throw new NotSupportedException("Choose Claude Code or Codex in Configure.");
        var server = JsonNode.Parse(File.ReadAllText(config))?["mcpServers"]?["launchbox"];
        var url = S(server, "url") ?? throw new InvalidDataException("Missing MCP address.");
        var bearer = S(server?["headers"], "Authorization");
        if (bearer == null || !bearer.StartsWith("Bearer ", StringComparison.Ordinal)) throw new InvalidDataException("Missing MCP token.");
        var start = Command(cli, cwd, CodexArguments(url, resume));
        start.Environment["PADMASTER_MCP_TOKEN"] = bearer[7..]; return start;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            if (_worker == null) { _worker = Task.Run(Run); _wake.Writer.TryWrite(true); }
        }
    }
    public SendReceipt Send(string text)
    {
        SendReceipt receipt;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
            var message = new Message(++_ticket, text, false); _lastUserText = text; _pending.Add(message);
            _queuePaused = false; var queued = _turn || _restarting || _pending.Count > 1;
            Next(); Publish(); Start();
            receipt = new(message.Ticket, queued);
        }
        Fire(); return receipt;
    }
    public void SendHidden(string text)
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) return;
            var index = _pending.FindLastIndex(m => m.Hidden) + 1;
            _pending.Insert(index, new(++_ticket, text, true)); Next(); Publish(); Start();
        }
        Fire();
    }
    void Next()
    {
        if (_turn || _restarting || _queuePaused || _pending.Count == 0 || _stop.IsCancellationRequested) return;
        _current = _pending[0]; _pending.RemoveAt(0); BeginTurn();
        var opened = _current; opened.Generation = _generation;
        Queue(() => TurnStarted?.Invoke(opened.Ticket, opened.Generation, opened.Hidden));
        if (opened.Hidden) Queue(() => HiddenTurn?.Invoke());
        WriteCurrent(); if (_child == null && !_attemptActive) _wake.Writer.TryWrite(true);
    }
    public void BeginTurn()
    {
        lock (_gate)
        {
            _order.Clear(); _messages.Clear(); _tools.Clear(); _message = ""; _anonymous = 0; _turn = true; _reportedFailure = false; _timedOut = false;
            if (Kind == "codex") { _codexCompleted = false; _attemptErrorResult = false; }
            _notice = ""; _canRetry = false; _usedWrites = false; _stderrLines = 0; _lastEvent = _turnStart = Now(); _generation++;
            _lines = 0; _stalls = 0; _firstLineMs = -1; _countsLogged = false; _kinds.Clear();
            _deadline?.Dispose(); _deadline = new Timer(_ => CheckTimeout(), null, WatchdogInterval, WatchdogInterval); Publish();
        }
        Fire();
    }
    public void CheckTimeout()
    {
        Process? child = null;
        lock (_gate)
        {
            if (!_turn || _held || _stop.IsCancellationRequested) return;
            var now = Now();
            if (now - _turnStart < TurnTimeout && (_activeTools > 0 || now - _lastEvent < SilenceTimeout)) return;
            _timedOut = true; child = _child; _exitNotified = true; _restarting = child != null;
            log($"turn watchdog: generation={_generation}, silent={(int)(now - _lastEvent).TotalSeconds}s, elapsed={(int)(now - _turnStart).TotalSeconds}s, tools={_activeTools}, lines={_lines}, child={child != null}");
            EndFailedTurn("no answer from the assistant", true);
        }
        Kill(child); Fire();
    }
    public void ToolActivity(bool started, bool writes)
    {
        lock (_gate)
        {
            _lastEvent = Now(); _activeTools = Math.Max(0, _activeTools + (started ? 1 : -1));
            if (_turn && writes) _usedWrites = true;
            Publish();
        }
        Fire();
    }
    public void SetHeld(bool held)
    {
        lock (_gate) { _held = held; _lastEvent = _turnStart = Now(); Publish(); }
        Fire();
    }
    public void StopTurn()
    {
        Process? child;
        lock (_gate)
        {
            child = _child; _restarting = child != null; _exitNotified = true;
            EndFailedTurn("the assistant restarted; send that again", false);
        }
        Kill(child); Fire();
    }
    public void Retry()
    {
        Process? child;
        lock (_gate)
        {
            if (!_canRetry || _retryMessage == null || _stop.IsCancellationRequested) return;
            _canRetry = false; _queuePaused = false; _restarting = _child != null; child = _child; _exitNotified = child != null;
            var index = _pending.FindLastIndex(m => m.Hidden) + 1;
            _pending.Insert(index, _retryMessage); _retryMessage = null; _notice = "restarting the assistant…"; Publish();
            if (child == null) { _restarting = false; Next(); Start(); }
        }
        Kill(child); Fire();
    }
    void EndFailedTurn(string message, bool retry)
    {
        _retryMessage = _current is { Hidden: false } ? _current : _retryMessage;
        _turn = false; _held = false; _queuePaused = true; _canRetry = retry && _retryMessage != null; _notice = message;
        _current = null; _deadline?.Dispose(); _activeTools = 0; LogCounts("failed");
        if (!_reportedFailure) { _reportedFailure = true; var text = message; Queue(() => Failure?.Invoke(text)); }
        Queue(() => Done?.Invoke(false)); Publish();
    }
    void WriteCurrent()
    {
        if (_child == null || _restarting || !_turn || _current == null || _sentGeneration == _generation) return;
        var process = _child; var generation = _generation; var message = _current; _sentGeneration = generation;
        _ = Write(process, message, generation);
    }
    async Task Write(Process process, Message message, int generation)
    {
        try
        {
            var line = Kind == "codex" ? message.Text : JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = message.Text } });
            await process.StandardInput.WriteLineAsync(line.AsMemory(), _stop.Token); await process.StandardInput.FlushAsync(_stop.Token);
            if (Kind == "codex") process.StandardInput.Close();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e)
        {
            log($"assistant send: {SafeDiagnostics.Error(e)}"); Kill(process); return;
        }
        log($"turn sent: generation={generation}, length={message.Text.Length}, hidden={message.Hidden}");
    }
    async Task Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await _wake.Reader.ReadAsync(_stop.Token); } catch (Exception e) when (e is OperationCanceledException or ChannelClosedException) { break; }
            while (_wake.Reader.TryRead(out _)) { }
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            Process? process = null; Task? errors = null; bool started = false; int? exitCode = null;
            string? resume;
            lock (_gate)
            {
                if (Kind == "codex" && (!_turn || _current == null)) continue;
                resume = LastSessionId; _resumedAttempt = resume != null; _attemptInitializations = _initializations; _attemptErrorResult = false; _codexCompleted = false; _exitNotified = false; _attemptActive = true;
            }
            try
            {
                process = new Process { StartInfo = ChatCommand(cli, cwd, mcpConfig, resume, Kind) };
                _stop.Token.ThrowIfCancellationRequested(); Launch(process); started = true; ChildJobs.Join(process, log);
                lock (_gate) { _child = process; Starts++; _restarting = false; WriteCurrent(); Publish(); }
                Fire();
                log($"assistant started pid={process.Id}, resumed={resume != null}");
                errors = DrainErrors(process, attempt.Token);
                while (await process.StandardOutput.ReadLineAsync(attempt.Token) is { } line) AcceptLine(line);
                log($"assistant stream ended: generation={Generation}, lines={Lines}, reason=end of output");
                await process.WaitForExitAsync(attempt.Token); exitCode = process.ExitCode;
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested) { log($"assistant stream ended: generation={Generation}, lines={Lines}, reason=stopped"); }
            catch (Exception e) { log($"assistant stream ended: generation={Generation}, lines={Lines}, reason=error"); log($"assistant process: {SafeDiagnostics.Error(e)}"); }
            finally
            {
                attempt.Cancel(); Kill(process);
                if (errors != null) try { await errors; } catch (OperationCanceledException) { } catch (Exception e) { log($"assistant stderr: {SafeDiagnostics.Error(e)}"); }
                if (started && process != null) try { if (process.WaitForExit(100)) { exitCode = process.ExitCode; log($"assistant exited code={exitCode}"); } } catch { }
                lock (_gate)
                {
                    _attemptActive = false;
                    if (ReferenceEquals(_child, process)) _child = null;
                    var completed = Kind == "codex" && _codexCompleted && !_attemptErrorResult && exitCode == 0;
                    if (resume != null && started && !_timedOut && !_exitNotified && _attemptInitializations == _initializations && (Kind == "codex" ? !completed : exitCode is not null and not 0 && _attemptErrorResult)) LastSessionId = null;
                    _resumedAttempt = false; _restarting = false;
                    if (!_exitNotified && !_stop.IsCancellationRequested && completed) CompleteCodexTurn();
                    else if (!_exitNotified && !_stop.IsCancellationRequested) EndFailedTurn("the assistant restarted; send that again", Kind == "codex");
                    else { _turn = false; _deadline?.Dispose(); Publish(); }
                    if (!completed || _exitNotified) Queue(() => Interrupted?.Invoke());
                    if (!_queuePaused && !_stop.IsCancellationRequested) Next();
                }
                Fire();
                process?.Dispose();
            }
        }
    }
    async Task DrainErrors(Process process, CancellationToken token)
    {
        while (await process.StandardError.ReadLineAsync(token) is { } line) AcceptError(line);
    }
    public void AcceptError(string line)
    {
        if (Kind == "codex") return;
        lock (_gate)
        {
            if (_stderrLines++ >= 20) return;
            var current = _current?.Text;
            var redacted = current is { Length: >= 4 } && line.Contains(current, StringComparison.Ordinal) || _lastUserText.Length >= 4 && line.Contains(_lastUserText, StringComparison.Ordinal);
            log(redacted ? $"child: <redacted, {line.Length} chars>" : "child: " + line[..Math.Min(line.Length, 500)]);
        }
    }
    public void AcceptLine(string line)
    {
        if (!Monitor.TryEnter(_gate, 1000))
        {
            var waited = Stopwatch.StartNew(); Monitor.Enter(_gate); waited.Stop();
            if (++_stalls == 1) log($"assistant stream stalled: the session lock waited {1000 + waited.ElapsedMilliseconds} ms, generation={_generation}, lines={_lines}");
        }
        try
        {
            _lastEvent = Now();
            if (_turn && ++_lines == 1) _firstLineMs = (int)(_lastEvent - _turnStart).TotalMilliseconds;
            AcceptLocked(line);
        }
        finally { Monitor.Exit(_gate); }
        Fire();
    }

    void AcceptLocked(string line)
    {
        try
        {
            var root = JsonNode.Parse(line) as JsonObject;
            if (root == null) { log($"ignored assistant output (expected an object): length={line.Length}"); return; }
            Count(S(root, "type"));
            if (Kind == "codex") { AcceptCodex(root); return; }
            if (root["parent_tool_use_id"] != null) return;
            switch (S(root, "type"))
            {
                case "system":
                    if (S(root, "subtype") == "init") { _initializations++; LastSessionId = S(root, "session_id") ?? LastSessionId; }
                    break;
                case "stream_event":
                    var ev = root["event"] as JsonObject;
                    switch (S(ev, "type"))
                    {
                        case "message_stop":
                            _message = "";
                            break;
                        case "message_start":
                            _message = S(ev?["message"], "id") ?? $"message-{++_anonymous}";
                            break;
                        case "content_block_start":
                            var block = ev?["content_block"];
                            if (S(block, "type") == "tool_use") Tool(block);
                            else if (S(block, "type") == "text" && S(block, "text") is { Length: > 0 } initial) Append(initial);
                            break;
                        case "content_block_delta":
                            if (S(ev?["delta"], "type") == "text_delta" && S(ev?["delta"], "text") is { } delta) Append(delta);
                            break;
                    }
                    break;
                case "assistant":
                    if (root["error"] != null)
                    {
                        log($"assistant error event: length={line.Length}, body omitted");
                        Queue(() => Failure?.Invoke("The assistant could not finish that reply. Please try again."));
                        break;
                    }
                    var message = root["message"] as JsonObject;
                    var id = S(message, "id") ?? (_message.Length > 0 ? _message : $"message-{++_anonymous}");
                    var text = new StringBuilder();
                    if (message?["content"] is JsonArray blocks)
                        foreach (var block in blocks.OfType<JsonObject>())
                        {
                            if (S(block, "type") == "text") text.Append(S(block, "text"));
                            else if (S(block, "type") == "tool_use") Tool(block);
                        }
                    if (text.Length > 0) SetText(id, text.ToString());
                    break;
                case "result":
                    var isError = B(root, "is_error") == true;
                    log($"result seen: generation={_generation}, length={line.Length}, subtype={Word(S(root, "subtype"))}, is_error={isError}, lines={_lines}"); LogCounts("result");
                    if (isError) _attemptErrorResult = true;
                    if (isError && _resumedAttempt && _attemptInitializations == _initializations)
                    {
                        log($"assistant resume error result: length={line.Length}, body omitted"); break;
                    }
                    LastSessionId = S(root, "session_id") ?? LastSessionId;
                    var success = S(root, "subtype") == "success" && !isError;
                    if (success && _order.Count == 0 && S(root, "result") is { Length: > 0 } result) SetText("result", result);
                    if (success) Queue(() => Activity?.Invoke(""));
                    else { log($"assistant error result: length={line.Length}, body omitted"); Queue(() => Failure?.Invoke("The assistant could not finish that reply. Please try again.")); }
                    _turn = false; _current = null; _deadline?.Dispose();
                    if (success) { _reportedFailure = false; _notice = ""; _canRetry = false; } else { _queuePaused = true; _notice = "The assistant could not finish that reply. Please try again."; }
                    var ended = success; Queue(() => Done?.Invoke(ended)); Publish(); if (success) Next();
                    break;
            }
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            log($"ignored assistant output: length={line.Length}, invalid JSON, {SafeDiagnostics.Error(e)}");
        }
    }

    void AcceptCodex(JsonObject root)
    {
        switch (S(root, "type"))
        {
            case "thread.started":
                if (S(root, "thread_id") is { Length: > 0 } id) { _initializations++; LastSessionId ??= id; }
                break;
            case "item.started":
            case "item.completed":
                if (!_turn || _codexCompleted || _attemptErrorResult || root["item"] is not JsonObject item) break;
                if (S(item, "type") == "agent_message" && S(root, "type") == "item.completed" && S(item, "text") is { Length: > 0 } text)
                    SetText(S(item, "id") ?? $"message-{++_anonymous}", text);
                else if (S(item, "type") == "mcp_tool_call")
                    Tool(new JsonObject { ["id"] = S(item, "id"), ["name"] = S(item, "tool"), ["input"] = item["arguments"]?.DeepClone() });
                break;
            case "turn.completed":
                if (!_turn || _codexCompleted || _attemptErrorResult) break;
                _codexCompleted = true;
                if (!_attemptActive) CompleteCodexTurn();
                break;
            case "turn.failed":
            case "error":
                _attemptErrorResult = true;
                if (_turn && !_attemptActive) EndFailedTurn("the assistant restarted; send that again", true);
                break;
        }
    }

    void CompleteCodexTurn()
    {
        LogCounts("completed"); _turn = false; _current = null; _deadline?.Dispose(); _activeTools = 0;
        _reportedFailure = false; _notice = ""; _canRetry = false;
        Queue(() => Activity?.Invoke("")); Queue(() => Done?.Invoke(true)); Publish();
    }

    void Append(string text)
    {
        if (_message.Length == 0) _message = $"message-{++_anonymous}";
        SetText(_message, _messages.GetValueOrDefault(_message, "") + text);
    }

    void SetText(string id, string text)
    {
        if (!_messages.ContainsKey(id)) _order.Add(id);
        _messages[id] = text;
        var joined = string.Join("\n\n", _order.Select(key => _messages[key]));
        LastReply = joined; ReplyRevision++; Queue(() => Reply?.Invoke(joined));
    }

    void Tool(JsonNode? block)
    {
        var name = S(block, "name") ?? "";
        if (name.StartsWith("mcp__launchbox__", StringComparison.Ordinal)) name = name[16..];
        var input = block?["input"] as JsonObject;
        if (name is "lb_get" or "lb_set" or "lb_create" && S(input, "type") == null || name == "lb_call" && S(input, "method") == null) return;
        var id = S(block, "id");
        if (id != null && !_tools.Add(id)) return;
        var type = BulkChanges.PlainType(S(input, "type"));
        Queue(() => Activity?.Invoke(name switch
        {
            "lb_catalog" => "reading the LaunchBox API…", "lb_list" => "searching your library…",
            "lb_get" => $"reading {type}…", "lb_set" => $"changing {type}…", "lb_create" => $"creating {type}…",
            "lb_call" => S(input, "method") switch { "PlayGame" => "launching the game…", "RefreshData" => "refreshing LaunchBox…", "Save" => "saving your library…", "GetAllGames" => "searching your library…", _ => "working on LaunchBox…" },
            "games" => "looking up games…", "platforms" => "looking up platforms…", "game" => "looking up the game…",
            "emulator" => "checking the emulator…", "show_image" => "showing an image…", "status" => "checking LaunchBox…", _ => "working…"
        }));
    }

    static string? S(JsonNode? node, string name) => node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    static bool? B(JsonNode? node, string name) => node is JsonObject obj && obj[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    public static ProcessStartInfo Command(string cli, string cwd, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(cli) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
        if (Path.GetExtension(cli).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var parts = new[] { cli }.Concat(args).ToArray();
            if (parts.Any(x => x.IndexOfAny(['"', '\r', '\n', '%', '!']) >= 0)) throw new ArgumentException("Unsupported character in command path.");
            start.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            start.Arguments = "/d /s /c \"" + string.Join(" ", parts.Select(x => "\"" + x + "\"")) + "\"";
        }
        else foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CLAUDE_CODE_USE_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase) || name.Equals("CODEX_API_KEY", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(name);
        return start;
    }

    public static async Task<AuthState> SignedIn(string cli, string cwd, Action<string> log, CancellationToken token = default, string kind = "claude-code")
    {
        var limit = TimeSpan.FromSeconds(10);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(limit);
        Process? process = null;
        try
        {
            process = new Process { StartInfo = Command(cli, cwd, StatusArguments(kind)) };
            process.Start(); ChildJobs.Join(process, log);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = await output;
            var errorText = await errors;
            return kind == "codex" ? AuthState.ParseCodex(text + errorText, process.ExitCode) : AuthState.Parse(text);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return AuthState.Unknown; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { log($"auth probe timed out ({limit.TotalSeconds:0} s)"); return AuthState.Unknown; }
        catch (Exception e) { log($"assistant auth unavailable: {SafeDiagnostics.Error(e)}"); return AuthState.Unknown; }
        finally { Kill(process); process?.Dispose(); }
    }

    public static ProcessStartInfo LoginCommand(string cli, string cwd, string kind = "claude-code")
    {
        var start = new ProcessStartInfo(cli) { WorkingDirectory = cwd, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal };
        foreach (var argument in kind == "codex" ? new[] { "login" } : new[] { "auth", "login", "--claudeai" }) start.ArgumentList.Add(argument);
        return start;
    }

    public static void WriteMcpConfig(string path, int port, string token) => File.WriteAllText(path,
        JsonSerializer.Serialize(new { mcpServers = new { launchbox = new { type = "http", url = $"http://127.0.0.1:{port}/mcp", headers = new { Authorization = "Bearer " + token } } } }), new UTF8Encoding(false));

    static void Kill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(true); } catch { }
    }

    public void Shutdown()
    {
        var clock = Stopwatch.StartNew(); Dispose();
        var worker = Volatile.Read(ref _worker) ?? Task.CompletedTask;
        try
        {
            if (worker.Wait(TimeSpan.FromMilliseconds(200))) return;
            var kill = Task.Run(() => Kill(Volatile.Read(ref _child)));
            var remaining = TimeSpan.FromMilliseconds(500) - clock.Elapsed;
            if (remaining > TimeSpan.Zero) Task.WhenAll(worker, kill).Wait(remaining);
        }
        catch (Exception e) { log($"assistant shutdown: {SafeDiagnostics.Error(e)}"); }
    }

    public void Dispose()
    {
        _stop.Cancel(); _wake.Writer.TryComplete();
        lock (_gate) { _turn = false; _held = false; _deadline?.Dispose(); _raise.Clear(); }
    }
}

public sealed class SignInFlow(Action launch, Func<CancellationToken, Task<AuthState>> check, Action<string> log, TimeSpan? interval = null, TimeSpan? duration = null) : IDisposable
{
    readonly CancellationTokenSource _stop = new();
    int _waiting;
    public Func<bool> Finished { get; init; } = () => true;
    public bool Waiting => Volatile.Read(ref _waiting) != 0;
    public event Action<string>? Message;
    public event Action<bool>? WaitingChanged;
    public event Action? Authenticated;

    public async Task Start()
    {
        if (_stop.IsCancellationRequested || Interlocked.CompareExchange(ref _waiting, 1, 0) != 0) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(duration ?? TimeSpan.FromMinutes(5));
        try
        {
            launch();
            WaitingChanged?.Invoke(true);
            Message?.Invoke("waiting for you to finish signing in in the browser…");
            while (true)
            {
                await Task.Delay(interval ?? TimeSpan.FromSeconds(3), deadline.Token);
                var auth = await check(deadline.Token);
                if (deadline.IsCancellationRequested) break;
                if (auth.SignedIn) { Authenticated?.Invoke(); return; }
                if (Finished() && auth.OtherAccount) { Message?.Invoke(auth.Reason + " Check its user settings, then try Sign in again."); return; }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        catch (Exception e) { log($"sign in: {SafeDiagnostics.Error(e)}"); if (!_stop.IsCancellationRequested) Message?.Invoke("Sign in could not open. Please try again."); return; }
        finally { Interlocked.Exchange(ref _waiting, 0); WaitingChanged?.Invoke(false); }
        if (!_stop.IsCancellationRequested) Message?.Invoke("Sign in took too long. Click Sign in to try again.");
    }

    public void Dispose() { _stop.Cancel(); }
}
