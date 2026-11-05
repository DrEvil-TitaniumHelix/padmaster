using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace PadMaster;

public class PadMasterPlugin : ISystemEventsPlugin
{
    static readonly string DirectoryPath = Path.GetDirectoryName(typeof(PadMasterPlugin).Assembly.Location)!;
    static readonly HostGate Host = new();
    static readonly DateTime? Loaded = Stamp();
    static int _changeReported;
    static DateTime? Stamp() { try { return File.GetLastWriteTimeUtc(typeof(PadMasterPlugin).Assembly.Location); } catch { return null; } }
    static void WatchFiles()
    {
        if (Loaded == null || Stamp() is not { } now || now == Loaded || Interlocked.Exchange(ref _changeReported, 1) != 0) return;
        W("plugin files changed since this LaunchBox started; close LaunchBox and start it again to load them");
    }
    static bool Allowed => Host.AllowsAssistant(PluginHelper.BigBoxMainViewModel != null, W);
    static readonly HashSet<int> BusyPorts = [];
    static Stopwatch? _startup;
    static long _mcpMs = -1, _panelMs = -1, _uiTicks;
    static int _dispatchDepth;
    static bool _startupLogged;
    static Settings _settings = new();
    static readonly BulkChanges Bulk = new(() => _settings, ShowBulk, BulkCompleted);
    static Mcp? _mcp;
    static Session? _session;
    static ChatBridge? _bridge;
    static SignInFlow? _login;
    static Process? _loginProcess;
    static SetupState _setupState = new("", null, false);
    static CancellationTokenSource? _configure;
    static AssistantPanel? _panel;
    static AssistantWindow? _assistant;
    static PanelPlacement? _placement;
    static DispatcherTimer? _poll;
    static PlacementDriver? _driver;
    static bool Opened => _driver?.Opened == true;
    static bool _initialized;
    static volatile bool _stopping;
    static string? _cli;
    static string _status = "offline", _mcpFailure = "";
    static readonly AuthCoordinator Auth = new();
    static string? _root;
    static ActivationReader? _activationReader;
    static System.Threading.Timer? _watch;
    static readonly object ReadGate = new();
    static readonly HashSet<string> ReadErrors = [];
    static long _readVersion, _appliedVersion;
    static int _reading;
    static readonly AssistantLifecycle Lifecycle = new(
        settings => { _settings = settings; _start = DateTime.UtcNow; StartMcp(); }, EnsurePanel,
        settings => { _panel!.LoadConfiguration(settings); if (!Opened) _panel.SetExpanded(settings.Expanded); }, () => _panel!.OpenConfiguration(),
        () => _poll?.Start(), startup => { _driver!.Open(_settings.Placement, startup); RecordPanelShown(); UpdateStatus(); }, () => ConfigureAssistant(), StopActive);
    static readonly ActivationGate Gate = new(Lifecycle.Start, ApplySettings, Lifecycle.Stop, text => MessageBox.Show(text, PackageIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Information));
    static string StorePath => PackageIdentity.StorePath(_root ?? throw new InvalidOperationException("LaunchBox root is unavailable."));
    static DateTime _start;
    static Window? _win;
    static Image? _img;

    public void OnEventRaised(string eventType)
    {
        try
        {
            if (!Host.AllowsAssistant(eventType == SystemEventTypes.BigBoxStartupCompleted || PluginHelper.BigBoxMainViewModel != null, W)) return;
            if (eventType == SystemEventTypes.LaunchBoxStartupCompleted) { _startup ??= Stopwatch.StartNew(); Dispatch(Up); }
            else if (eventType == SystemEventTypes.LaunchBoxShutdownBeginning) Down();
        }
        catch (Exception e) { W($"ERROR host event: {SafeDiagnostics.Error(e)}"); }
    }

    static void Up()
    {
        if (!Allowed || _initialized || _stopping) return;
        _initialized = true;
        if (Application.Current is { } app)
        {
            app.Exit += (_, _) => Down();
            if (app.MainWindow is { } main) main.Closed += (_, _) => Down();
        }
        _watch = new System.Threading.Timer(_ => Watch(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    static (long version, ActivationState state) ReadActivation()
    {
        lock (ReadGate)
        {
            try { _root ??= PackageIdentity.RootFromBridge(DirectoryPath); _activationReader ??= new ActivationReader(Path.Combine(_root, "Data", "Settings.xml"), StorePath, ReadWarning); return (++_readVersion, _activationReader.Read()); }
            catch (Exception e) { ReadWarning($"activation: {SafeDiagnostics.Error(e)}"); return (++_readVersion, new(new(), false, false)); }
        }
    }

    static void ReadWarning(string message)
    {
        lock (ReadErrors) if (ReadErrors.Add(message)) W(message.Replace('\r', ' ').Replace('\n', ' '));
    }

    static void ApplyActivation((long version, ActivationState state) snapshot)
    {
        if (!Allowed || _stopping || snapshot.version <= _appliedVersion) return;
        _appliedVersion = snapshot.version; Gate.Update(snapshot.state);
    }

    static void Watch()
    {
        if (!Allowed || _stopping || Interlocked.Exchange(ref _reading, 1) != 0) return;
        try { WatchFiles(); var snapshot = ReadActivation(); Dispatch(() => ApplyActivation(snapshot)); }
        catch (Exception e) { ReadWarning($"activation watch: {SafeDiagnostics.Error(e)}"); }
        finally { Volatile.Write(ref _reading, 0); }
    }

    static void OpenPanel(bool configure)
    {
        if (!Allowed) return;
        if (_mcp == null && !BusyPorts.Contains(_settings.Port)) StartMcp();
        Lifecycle.Open(_settings, configure);
    }

    static void EnsurePanel()
    {
        if (!Allowed || _panel != null) return;
        _panel = new AssistantPanel { Log = W, SettingsRequested = OpenConfiguration, ConfigurationSaved = SaveConfiguration, HelpRequested = OpenHelp, Reopened = () => RequestOpen(false) };
        _panel.IsVisibleChanged += (_, _) => Dispatch(RecordPanelShown);
        _panel.SendRequested = text => { if (!Gate.Active || _session == null) throw new InvalidOperationException("Assistant is unavailable."); _session.Send(text); };
        _panel.ExpandedChanged = async expanded =>
        {
            if (!Gate.Active) return;
            try { await Settings.UpdateAsync(StorePath, current => current with { Expanded = expanded }); ApplyActivation(await Task.Run(ReadActivation)); }
            catch (Exception e) { W($"Expand state was not saved because settings could not be read or written: {SafeDiagnostics.Error(e)}"); }
        };
        var app = Application.Current;
        var main = app?.MainWindow;
        _assistant = new AssistantWindow();
        if (main != null)
        {
            _assistant.Owner = main;
        }
        _assistant.UserClosed += () => _driver?.CloseFloating();
        _placement = new PanelPlacement(_panel, _assistant, W);
        _driver = new PlacementDriver(_placement, ReadHost);
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Dispatch(Poll), _panel.Dispatcher);
    }

    static void ApplySettings(Settings previous, Settings next)
    {
        if (!next.Enabled) Bulk.Invalidate("The assistant was disabled. Nothing changed.");
        _settings = next; _panel?.LoadConfiguration(next); _panel?.SetExpanded(next.Expanded);
        var restart = previous.Port != next.Port || previous.Cli != next.Cli || previous.Assistant != next.Assistant || _session?.Paused == true;
        if (previous.Port != next.Port) StartMcp();
        Lifecycle.Saved(previous, next, _driver?.Shown == true, W);
        if (restart) { StopSession(); if (Opened) ConfigureAssistant(true); }
    }

    static void StopActive()
    {
        Bulk.Invalidate("The assistant was disabled. Nothing changed.");
        _configure?.Cancel(); CancelLogin(); StopSession(); _poll?.Stop(); _mcp?.Stop(); _mcp = null; _driver?.Stop();
        try { _placement?.Detach(); } catch (Exception e) { W($"detach while disabled: {SafeDiagnostics.Error(e)}"); }
        _assistant?.Hide(); _panel?.Suspend(); _win?.Close(); W("assistant disabled; mcp down");
    }

    static void Down()
    {
        if (_stopping) return;
        _stopping = true; Bulk.Invalidate("The assistant is shutting down. Nothing changed."); ChildJobs.Shutdown(); _watch?.Dispose(); _configure?.Cancel(); CancelLogin(); _session?.Shutdown(); _mcp?.Stop(); _mcp = null;
        Dispatch(() =>
        {
            _poll?.Stop();
            try { _placement?.Detach(); } catch (Exception e) { W($"detach on shutdown: {SafeDiagnostics.Error(e)}"); }
            _panel?.Shutdown(); _assistant?.Shutdown(); _win?.Close();
        });
        W("mcp down");
    }

    static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        void Safe()
        {
            var started = Stopwatch.GetTimestamp(); _dispatchDepth++;
            try { action(); } catch (Exception e) { W($"UI: {SafeDiagnostics.Error(e)}"); }
            finally
            {
                if (--_dispatchDepth == 0 && !_startupLogged && _startup != null)
                {
                    _uiTicks += Stopwatch.GetTimestamp() - started;
                    if (_mcpMs >= 0 && _panelMs >= 0) { _startupLogged = true; W($"startup: mcp {_mcpMs} ms, panel {_panelMs} ms, ui {_uiTicks * 1000 / Stopwatch.Frequency} ms"); }
                }
            }
        }
        if (dispatcher.CheckAccess()) Safe(); else _ = dispatcher.BeginInvoke(Safe);
    }

    static void RecordPanelShown()
    {
        if (_panelMs < 0 && _panel?.IsVisible == true) _panelMs = _startup?.ElapsedMilliseconds ?? -1;
    }

    static void StartMcp()
    {
        if (!Allowed || _stopping) return;
        _mcp?.Stop(); _mcp = null; _mcpFailure = "";
        if (BusyPorts.Contains(_settings.Port)) { _mcpFailure = $"port {_settings.Port} in use - change it in Configure"; UpdateStatus(); return; }
        var mcp = new Mcp(_settings.Port, "/mcp", Tools());
        try
        {
            mcp.Start(); Session.WriteMcpConfig(Path.Combine(DirectoryPath, "mcp.json"), _settings.Port, mcp.Token); _mcp = mcp;
            if (_mcpMs < 0) _mcpMs = _startup?.ElapsedMilliseconds ?? -1;
            W($"mcp up http://127.0.0.1:{_settings.Port}/mcp");
        }
        catch (Exception e)
        {
            mcp.Stop();
            _mcpFailure = FailureText.Listener(e, _settings.Port);
            if (!_mcpFailure.StartsWith("port ", StringComparison.Ordinal) || BusyPorts.Add(_settings.Port)) W("mcp start: " + _mcpFailure + "; " + SafeDiagnostics.Error(e));
            _status = "offline · " + _mcpFailure; UpdateStatus();
        }
    }

    static void StopSession()
    {
        Bulk.Invalidate("The session restarted. Nothing changed.");
        var session = _session; _session = null; _bridge = null; Auth.Reset(); session?.Dispose(); _panel?.SetReady(false); _panel?.Idle();
    }

    static async void ConfigureAssistant(bool restart = false, bool signIn = false)
    {
        try
        {
            if (!Allowed || !Gate.Active || !Opened || _stopping) return;
            _configure?.Cancel();
            var cancellation = _configure = new CancellationTokenSource();
            if (restart) { CancelLogin(); _panel?.SetSetupWaiting(false); StopSession(); }
            if (_session == null) { _panel?.SetReady(false); _panel?.SetSetup(new(_settings.Assistant, _cli, false, Checking: true)); }
            if (File.Exists(Path.Combine(DirectoryPath, "nolaunch")))
            {
                CancelLogin(); _panel?.SetSetupWaiting(false); StopSession(); _setupState = new(_settings.Assistant, null, false, true);
                _panel?.SetSetup(_setupState); _status = "offline"; UpdateStatus(); W("assistant skipped (nolaunch)"); return;
            }
            var kind = _settings.Assistant;
            var detected = await Task.Run(() => Settings.DetectCli(_settings.Cli, kind), cancellation.Token);
            if (cancellation.IsCancellationRequested || _stopping || !Gate.Active) return;
            _cli = detected;
            _status = AuthCoordinator.CheckingStatus(_session != null, Auth.ConnectedStatus, _cli, _mcpFailure); UpdateStatus();
            if (_cli == null) { StopSession(); _setupState = new(_settings.Assistant, null, false); _panel?.SetSetup(_setupState); return; }
            var cli = _cli;
            var cwd = Path.Combine(DirectoryPath, "persona");
            await Task.Run(() => Directory.CreateDirectory(cwd), cancellation.Token);
            var auth = await Task.Run(() => Session.SignedIn(cli, cwd, W, cancellation.Token, kind), cancellation.Token);
            if (cancellation.IsCancellationRequested || _stopping) return;
            if (!Auth.Apply(auth, _session != null, _session?.Busy == true, StopSession, value =>
            {
                _setupState = new(_settings.Assistant, cli, value.SignedIn, Reason: value.Reason); _panel?.SetSetup(_setupState);
            }))
            {
                _status = Auth.Status; _panel?.SetReady(true); UpdateStatus(); return;
            }
            if (!_setupState.Ready)
            {
                if (auth.ShouldStop(_session?.Busy == true) || _session == null) StopSession(); _status = _mcp == null ? "offline · " + _mcpFailure : _settings.Assistant.Length == 0 ? "choose your AI" : "not signed in"; UpdateStatus();
                if (signIn && _setupState.CanSignIn) SignIn();
                return;
            }
            if (_mcp == null) { StopSession(); _status = "offline · " + _mcpFailure; UpdateStatus(); return; }
            var status = await Task.Run(() =>
            {
                var games = Dm.GetAllGames();
                return $"connected · {Settings.AssistantName(kind)} · {games.Length:N0} games · NES {games.Count(g => g.Platform == "Nintendo Entertainment System" || g.Platform == "NES"):N0}";
            }, cancellation.Token);
            if (cancellation.IsCancellationRequested || _stopping) return;
            if (_session == null)
            {
                var config = Path.Combine(DirectoryPath, "mcp.json");
                if (cancellation.IsCancellationRequested || _stopping || !Gate.Active) return;
                var session = new Session(cli, cwd, config, W) { Kind = kind };
                _session = session;
                session.Interrupted += () => { if (ReferenceEquals(_session, session)) Bulk.Invalidate("The session stopped. Nothing changed."); };
                _bridge = new ChatBridge(session, _panel!, () => _session, () => _stopping, W, _ =>
                    Auth.BetweenTurns(session.Busy, StopSession, denied =>
                    {
                        _setupState = new(_settings.Assistant, _cli, false, Reason: denied.Reason); _panel!.SetSetup(_setupState); _status = "not signed in"; UpdateStatus();
                    }));
                session.Start();
            }
            _status = Auth.ConnectedStatus = status; _panel?.SetReady(true); UpdateStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            W($"assistant configuration: {SafeDiagnostics.Error(e)}"); StopSession();
            var reason = FailureText.Configuration(e);
            _status = "offline · " + reason; _panel?.SetSetupMessage(reason); UpdateStatus();
        }
    }

    static void SignIn()
    {
        if (!Allowed || !Gate.Active || _cli == null || _stopping || !_setupState.CanSignIn || _login?.Waiting == true) return;
        try
        {
            CancelLogin();
            var cli = _cli; var cwd = Path.Combine(DirectoryPath, "persona"); var kind = _settings.Assistant;
            var login = new SignInFlow(() => _loginProcess = Process.Start(Session.LoginCommand(cli, cwd, kind)), token => Task.Run(() => Session.SignedIn(cli, cwd, W, token, kind), token), W) { Finished = () => _loginProcess == null || _loginProcess.HasExited };
            _login = login;
            void Ui(Action action) => Dispatch(() => { if (ReferenceEquals(_login, login) && !_stopping) action(); });
            login.Message += text => Ui(() => _panel?.SetSetupMessage(text));
            login.WaitingChanged += waiting => Ui(() => _panel?.SetSetupWaiting(waiting));
            login.Authenticated += () => Ui(() => ConfigureAssistant());
            _ = login.Start();
        }
        catch (Exception e) { W($"sign in launch: {SafeDiagnostics.Error(e)}"); _panel?.SetSetupMessage("Sign in could not open. Please try again."); }
    }

    static void CancelLogin()
    {
        var login = _login; _login = null; login?.Dispose();
        _loginProcess?.Dispose(); _loginProcess = null;
    }

    static async void SaveConfiguration(IReadOnlyDictionary<string, string?> fields, bool signIn)
    {
        try
        {
            if (!Gate.Active || _stopping) return;
            var settings = await Settings.UpdateAsync(StorePath, current =>
            {
                if (!Settings.TryApply(current, fields, false, out var value, out var error)) throw new ArgumentException(error);
                return value;
            });
            ApplyActivation(await Task.Run(ReadActivation));
            if (!Gate.Active || _stopping) return;
            _panel?.ConfigurationAccepted(settings); ConfigureAssistant(false, signIn);
        }
        catch (Exception e) { W($"setup save: {SafeDiagnostics.Error(e)}"); _panel?.SetSetupMessage("Setup could not be read or saved. Check Local/Plugins/.data, then try again."); }
    }

    static void OpenHelp()
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(DirectoryPath, "README.md")) { UseShellExecute = true }); }
        catch (Exception e) { W($"help: {SafeDiagnostics.Error(e)}"); _panel?.SetSetupMessage("Help could not open. Read README.md in the plugin folder."); }
    }

    public static void OpenAssistant() => Dispatch(() => RequestOpen(false));
    static void OpenConfiguration() => Dispatch(() => RequestOpen(true));
    static async void RequestOpen(bool configure)
    {
        try
        {
            if (!Allowed) return;
            Up(); if (_stopping) return;
            ApplyActivation(await Task.Run(ReadActivation));
            if (_stopping) return;
            Gate.Open(() => OpenPanel(configure));
        }
        catch (Exception e) { W($"open: {SafeDiagnostics.Error(e)}"); }
    }

    static PlacementHost ReadHost()
    {
        var model = PluginHelper.LaunchBoxMainViewModel;
        var details = model?.GameDetailsView as FrameworkElement;
        var visible = details is { IsVisible: true } && _placement?.IsRightHostVisible(details) != false;
        return new(model != null, Application.Current?.MainWindow, details, visible);
    }

    static void Poll()
    {
        if (!Allowed || !Gate.Active || !Opened || _stopping) return;
        try { _driver?.Refresh(); RecordPanelShown(); UpdateStatus(); _bridge?.Heal(); }
        catch (Exception e) { W($"placement poll: {SafeDiagnostics.Error(e)}"); }
    }

    static void UpdateStatus()
    {
        if (_mcpFailure.StartsWith("port ", StringComparison.Ordinal)) { _panel?.SetStatus(_mcpFailure); return; }
        var fallback = _driver?.HostUnavailable == true ? "LaunchBox view is unavailable; using floating window." : _placement?.Status;
        _panel?.SetStatus(_status + (string.IsNullOrEmpty(fallback) ? "" : " · " + fallback));
    }

    static bool ShowBulk(BulkPrompt prompt)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || _stopping) return false;
        var operation = dispatcher.InvokeAsync(() =>
        {
            if (!Allowed || !Gate.Active || _session == null || _stopping) return false;
            EnsurePanel(); _session?.SetHeld(true); _panel!.ShowBulk(prompt); _driver!.Open(_settings.Placement, false); _panel.UpdateLayout();
            return _driver.Shown && _panel.IsVisible && _panel.Expanded;
        });
        if (operation.Task.Wait(TimeSpan.FromSeconds(2))) return operation.Task.Result;
        operation.Abort(); return false;
    }

    static void BulkCompleted(string line, string? hidden)
    {
        var session = _session;
        session?.SetHeld(false);
        Dispatch(() =>
        {
            _panel?.BulkFinished(line, !Bulk.Waiting);
            if (hidden != null && session != null && ReferenceEquals(session, _session) && Gate.Active && !_stopping) session.SendHidden(hidden);
        });
    }

    static IDataManager Dm => PluginHelper.DataManager;

    static Tool Track(Tool tool)
    {
        var writes = tool.Description.StartsWith("WRITES", StringComparison.Ordinal);
        return tool with { Handler = arguments =>
        {
            var session = _session; session?.ToolActivity(true, writes);
            try { return tool.Handler(arguments); } finally { session?.ToolActivity(false, writes); }
        } };
    }

    static List<Tool> Tools() => [.. Hosted().Select(Track)];

    static List<Tool> Hosted() =>
    [
        .. Api.Host(DirectoryPath, W, Bulk).Tools(),
        new("show_image", "Show an image file in the Padmaster AI Assistant viewer", Obj(("path", true), ("title", false)), a =>
        {
            var path = S(a, "path") ?? throw new Exception("path required");
            LocalImage.Validate(path, File.Exists);
            Show(path, S(a, "title") ?? "Padmaster AI Assistant");
            return new JsonObject { ["shown"] = path };
        }),
        new("status", "Bridge status", Obj(), _ => new JsonObject
        {
            ["version"] = "b1", ["port"] = _settings.Port,
            ["uptimeSec"] = (int)(DateTime.UtcNow - _start).TotalSeconds,
            ["games"] = Dm.GetAllGames().Length,
        }),
    ];

    static JsonObject Emu(IEmulator e, string platform)
    {
        var ep = e.GetAllEmulatorPlatforms().FirstOrDefault(p => p.Platform == platform);
        return new JsonObject
        {
            ["id"] = e.Id, ["title"] = e.Title, ["exe"] = e.ApplicationPath,
            ["commandLine"] = string.IsNullOrWhiteSpace(ep?.CommandLine) ? e.CommandLine : ep.CommandLine,
        };
    }

    static void Show(string path, string title)
    {
        var app = Application.Current ?? throw new Exception("no WPF application");
        app.Dispatcher.Invoke(() =>
        {
            if (_win == null)
            {
                _img = new Image { Stretch = System.Windows.Media.Stretch.Uniform };
                _win = new Window { Title = title, Width = 900, Height = 600, Topmost = false, Content = _img };
                _win.Closed += (_, _) => { _win = null; _img = null; };
            }
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            _win.Title = title;
            _img!.Source = bmp;
            _win.Show();
        });
    }

    static string? S(JsonNode? a, string k) => a?[k]?.GetValue<string>();
    static JsonArray Arr(IEnumerable<JsonNode?> xs) => new(xs.ToArray());
    static JsonObject Brief(IGame g) => new() { ["id"] = g.Id, ["title"] = g.Title, ["platform"] = g.Platform };

    static JsonObject Obj(params (string name, bool required)[] ps)
    {
        var props = new JsonObject();
        foreach (var (n, _) in ps) props[n] = new JsonObject { ["type"] = "string" };
        var o = new JsonObject { ["type"] = "object", ["properties"] = props };
        var req = ps.Where(p => p.required).Select(p => (JsonNode)p.name).ToArray();
        if (req.Length > 0) o["required"] = new JsonArray(req);
        return o;
    }

    static void W(string s)
    {
        RotatingLog.Write(Path.Combine(DirectoryPath, "padmaster.log"), s);
    }
}

public sealed class AssistantMenu : ISystemMenuItemPlugin
{
    public string Caption => "Padmaster AI Assistant";
    public System.Drawing.Image IconImage => null!;
    public bool ShowInLaunchBox => true;
    public bool ShowInBigBox => false;
    public bool AllowInBigBoxWhenLocked => false;
    public void OnSelected() => PadMasterPlugin.OpenAssistant();
}
