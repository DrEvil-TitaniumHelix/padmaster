using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace PadMaster.Controls;

public sealed class MappingView : UserControl
{
    public IReadOnlyList<GroupBox> Cells { get; }
    public IReadOnlyList<RenderedState> States { get; }
    public ArtworkResult? Artwork { get; }
    public UniformGrid StateGrid { get; }
    public ComboBox Controller { get; }
    public TextBlock Source { get; }
    public TextBlock? ArtworkError { get; }
    public event Action<string>? ControllerPicked;   // controller id chosen in the dropdown (only when it differs from the drawn one)
    public event Action? AddControllerRequested;     // the last dropdown entry
    public event Action? ChangeControllerRequested, RemoveControllerRequested, RedoMapRequested;   // the three buttons beside the dropdown
    public Button Change { get; }
    public Button Remove { get; }
    public Button Redo { get; }
    public const string AddController = "Add my controller...";
    public MappingView(string controllerName, string sourcePath, IReadOnlyList<RenderedState> states, ArtworkResult? artwork = null, string? artworkError = null, IReadOnlyList<(string Id, string Name)>? controllers = null, string? controllerId = null)
    {
        States = states; Artwork = artwork;
        if (states.Count is < 1 or > 6) throw new InvalidDataException("A mapping must contain one to six states.");
        AutomationProperties.SetAutomationId(this, "PadMappingView");
        var panel = new DockPanel { Margin = new Thickness(12), LastChildFill = true };
        var pads = controllers is { Count: > 0 } ? controllers : [(controllerId ?? controllerName, controllerName)];
        var current = Math.Max(0, pads.ToList().FindIndex(p => string.Equals(p.Id, controllerId, StringComparison.OrdinalIgnoreCase)));
        Controller = new ComboBox { ItemsSource = pads.Select(p => p.Name).Append(AddController).ToArray(), SelectedIndex = current, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 240, Margin = new Thickness(0, 0, 0, 12) };
        Controller.SelectionChanged += (_, _) =>
        {
            var i = Controller.SelectedIndex; if (i < 0 || i == current) return;
            if (i < pads.Count) { ControllerPicked?.Invoke(pads[i].Id); return; }
            Controller.SelectedIndex = current; AddControllerRequested?.Invoke();   // the drawn pad stays selected; the new one arrives by a redraw
        };
        AutomationProperties.SetAutomationId(Controller, "PadMappingController");
        Button Tool(string text, string id, Action? click) { var b = new Button { Content = text, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 12), VerticalAlignment = VerticalAlignment.Top }; AutomationProperties.SetAutomationId(b, id); b.Click += (_, _) => click?.Invoke(); return b; }
        Change = Tool("Change this controller...", "PadMappingChange", () => ChangeControllerRequested?.Invoke());
        Remove = Tool("Remove this controller", "PadMappingRemove", () => RemoveControllerRequested?.Invoke());
        Redo = Tool("Redo this game's map", "PadMappingRedo", () => RedoMapRequested?.Invoke());
        var tools = new StackPanel { Orientation = Orientation.Horizontal }; tools.Children.Add(Controller); tools.Children.Add(Change); tools.Children.Add(Remove); tools.Children.Add(Redo);
        DockPanel.SetDock(tools, Dock.Top); panel.Children.Add(tools);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; Source = new TextBlock { Text = "Source: " + (sourcePath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "web " : "manual ") + sourcePath, TextWrapping = TextWrapping.Wrap }; footer.Children.Add(Source);
        if (artwork?.Retained.Count > 0) footer.Children.Add(new TextBlock { Text = "Kept artwork for earlier states: " + string.Join(", ", artwork.Retained.Keys), TextWrapping = TextWrapping.Wrap });
        if (artworkError != null) { ArtworkError = new TextBlock { Text = "LaunchBox artwork not updated: " + artworkError, TextWrapping = TextWrapping.Wrap }; footer.Children.Add(ArtworkError); }
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        StateGrid = new UniformGrid { Columns = Math.Min(states.Count, 3), Rows = (states.Count + 2) / 3 };
        var cells = new List<GroupBox>();
        foreach (var state in states)
        {
            var body = new StackPanel();
            Image Picture(string path, string view)
            {
                var bitmap = new BitmapImage(); using (var file = File.OpenRead(path)) { bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = file; bitmap.EndInit(); } bitmap.Freeze();
                var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(4) }; AutomationProperties.SetName(image, state.State.Name + " " + view); return image;
            }
            body.Children.Add(Picture(state.FrontPath, "front")); if (state.BackPath != null) body.Children.Add(Picture(state.BackPath, "back"));
            if (state.State.Unbound.Count > 0) body.Children.Add(new TextBlock { Text = "Not bound on this pad: " + string.Join("; ", state.State.Unbound), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) });
            var cell = new GroupBox { Header = state.State.Name, Content = body, Margin = new Thickness(4), Padding = new Thickness(4) };
            AutomationProperties.SetAutomationId(cell, "PadMappingState_" + cells.Count); AutomationProperties.SetName(cell, state.State.Name); cells.Add(cell); StateGrid.Children.Add(cell);
        }
        Cells = cells; panel.Children.Add(new ScrollViewer { Content = StateGrid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Content = panel;
    }
}

public sealed class ControlsPlugin : IGameMenuItemPlugin
{
    readonly string pluginDirectory;
    public ControlsPlugin() : this(Path.GetDirectoryName(typeof(ControlsPlugin).Assembly.Location)!) { }
    public ControlsPlugin(string pluginDirectory) { this.pluginDirectory = pluginDirectory; RotatingLog.Write(Path.Combine(pluginDirectory, "padmaster.log"), "controls plugin loaded " + (typeof(ControlsPlugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?")); }
    // Pad Master's own settings live in the one settings store (Configure form); the assistant's placement is separate from the menu item's text and the pad drawn.
    public static Func<string, string> StorePath { get; set; } = PackageIdentity.StorePath;   // seam: checks point it at a scratch root
    public static Settings Current(string launchBoxRoot) => Settings.Read(StorePath(launchBoxRoot), _ => { });
    static string RootOf(string pluginDirectory) => Path.GetFullPath(Path.Combine(pluginDirectory, "..", ".."));
    // One switch for the whole plugin: Configure > Enable. Before it, nothing of Pad Master shows (menu item, badge, pictures, play panel). Seam: checks set it.
    public static Func<string, bool> IsEnabled { get; set; } = dir => { try { return Current(RootOf(dir)).Enabled; } catch { return false; } };
    public string Caption { get { try { return Current(RootOf(pluginDirectory)).MenuCaption; } catch { return Settings.DefaultMenuCaption; } } }
    public static IReadOnlyList<(string Id, string Name)> Controllers(string pluginDirectory) =>
        Directory.GetDirectories(Path.Combine(pluginDirectory, "controllers")).Where(d => File.Exists(Path.Combine(d, "anchors.json"))).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => (Path.GetFileName(d), JsonNode.Parse(File.ReadAllText(Path.Combine(d, "anchors.json")))?["displayName"]?.GetValue<string>() ?? Path.GetFileName(d))).ToList();
    public bool SupportsMultipleGames => false;
    public bool ShowInLaunchBox => true;
    public bool ShowInBigBox => false;
    public System.Drawing.Image IconImage => null!;
    public bool GetIsValidForGame(IGame selectedGame) => selectedGame != null && IsEnabled(pluginDirectory);
    public bool GetIsValidForGames(IGame[] selectedGames) => false;
    public void OnSelected(IGame[] selectedGames) { }

    public void OnSelected(IGame selectedGame)
    {
        if (PluginHelper.BigBoxMainViewModel != null) return;
        void Open()
        {
            Window window;
            try
            {
                var dir = Path.GetDirectoryName(typeof(ControlsPlugin).Assembly.Location)!;
                window = BuildWindow(selectedGame, dir, Path.GetFullPath(Path.Combine(dir, "..", "..")), id => PluginHelper.DataManager.GetEmulatorById(id), () => PluginHelper.LaunchBoxMainViewModel?.RefreshData(), offer: true);
            }
            catch (Exception e) { window = MessageWindow(selectedGame.Title, e.Message); }
            try { if (Application.Current?.MainWindow is { IsVisible: true } owner) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; } } catch { }
            window.Show();
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke((Action)Open); else Open();
    }

    static Window MessageWindow(string title, string message) => new() { Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) }, Width = 560, Height = 180 };
    public static Window BuildWindow(IGame game, string pluginDirectory, string launchBoxRoot, Func<string, IEmulator?> findEmulator, Action refresh, bool offer = false)
    {
        var log = Path.Combine(pluginDirectory, "padmaster.log");
        try
        {
            var window = Build(game, pluginDirectory, launchBoxRoot, findEmulator, refresh, offer);
            RotatingLog.Write(log, window.Content is MappingView view ? $"controls: {game.Title} -> {view.States.Count} states, {view.Artwork?.Written.Count ?? 0} artwork files" : $"controls: {game.Title} -> no mapping");
            return window;
        }
        catch (Exception e) { RotatingLog.Write(log, $"controls: {game.Title} -> error {e.Message}"); return MessageWindow(game.Title, e.Message); }
    }

    static Readers.ReadResult ReadBindings(IGame game, IEmulator emulator, string launchBoxRoot)
    {
        var exe = Path.GetFullPath(emulator.ApplicationPath, launchBoxRoot);
        var command = !string.IsNullOrWhiteSpace(game.CommandLine) ? game.CommandLine : emulator.GetAllEmulatorPlatforms().FirstOrDefault(p => p.Platform == game.Platform)?.CommandLine ?? emulator.CommandLine ?? "";
        var rom = string.IsNullOrWhiteSpace(game.ApplicationPath) ? null : Path.GetFullPath(game.ApplicationPath, launchBoxRoot);
        return Readers.Select.ForExe(exe, command, rom, game.Platform);
    }

    public sealed record Resolved(IReadOnlyList<RenderedState> States, string ControllerId, string DisplayName, string SourcePath, string Directory);
    public static Resolved? Resolve(IGame game, IEmulator? emulator, string pluginDirectory, string launchBoxRoot, string? controllerId = null)
    {
        var json = Mapping.GamePath(pluginDirectory, game.Platform, game.Title); if (!File.Exists(json) || emulator == null) return null;
        string controller;
        try { controller = Mapping.Controller(pluginDirectory, controllerId ?? (Current(launchBoxRoot).Controller is { Length: > 0 } chosen ? chosen : null)); }
        catch (InvalidDataException) when (controllerId == null) { controller = Mapping.Controller(pluginDirectory); }   // the chosen folder is gone: draw on the default rather than fail
        controllerId = Path.GetFileName(controller);
        var doc = JsonNode.Parse(File.ReadAllText(json))!; var source = doc["source"]!;
        if (doc["game"]!.GetValue<string>() != game.Title || doc["platform"]!.GetValue<string>() != game.Platform || source["type"]!.GetValue<string>() is not ("manual" or "web")) return null;
        if (doc["states"]!.AsArray().Count is < 1 or > 6) throw new InvalidDataException("A mapping must contain one to six states.");
        var measured = Path.Combine(controller, "measured.json"); var anchors = JsonNode.Parse(File.ReadAllText(Path.Combine(controller, "anchors.json")))!;
        var displayName = anchors["displayName"]?.GetValue<string>() ?? (File.Exists(measured) ? JsonNode.Parse(File.ReadAllText(measured))?["controller"]?.GetValue<string>() : null) ?? controllerId;
        var directory = Mapping.RenderDirectory(pluginDirectory, game.Platform, game.Title);
        var read = ReadBindings(game, emulator, launchBoxRoot);
        if (read.Error != null || read.Bindings.Count == 0) return null;
        var logo = Mapping.Logo(Mapping.LogoTypes.SelectMany(t => game.GetAllImagesWithDetails(t)?.Select(i => i.FilePath) ?? []));
        return new(Mapping.Render(controller, json, read.Bindings, Path.Combine(directory, controllerId), read.Config, logo, emulator.Title), controllerId, displayName, source["path"]!.GetValue<string>(), directory);
    }

    public static (ArtworkResult? Artwork, string? Error) Publish(IGame game, Resolved resolved, string pluginDirectory, Action refresh)
    {
        var log = Path.Combine(pluginDirectory, "padmaster.log"); ArtworkResult? artwork = null; string? error = null;
        try { artwork = Artwork.Write(game, resolved.ControllerId, resolved.States, resolved.Directory); if (artwork.Writes > 0) RotatingLog.Write(log, $"artwork: {game.Title} -> {artwork.Writes} files"); Artwork.Refresh(resolved.Directory, refresh); }
        catch (Exception e) { error = e.Message; RotatingLog.Write(log, $"artwork: {game.Title} -> error {e.Message}"); }
        return (artwork, error);
    }

    internal static Window Build(IGame game, string pluginDirectory, string launchBoxRoot, Func<string, IEmulator?> findEmulator, Action refresh, bool offer = false)
    {
        var emulator = findEmulator(game.EmulatorId); var resolved = Resolve(game, emulator, pluginDirectory, launchBoxRoot);
        if (resolved == null && offer && !File.Exists(Mapping.GamePath(pluginDirectory, game.Platform, game.Title)))
            return emulator != null && ReadBindings(game, emulator, launchBoxRoot) is { Error: null, Bindings.Count: > 0 } read
                ? GenerateWindow.Ask(game, GenerateRequest.For(game, read.Bindings.Keys, launchBoxRoot), pluginDirectory, launchBoxRoot, findEmulator, refresh)
                : MessageWindow(game.Title, $"Pad Master can't draw {game.Title} yet: it doesn't read this game's emulator's button settings. Emulators it reads: RetroArch, PCSX2, Dolphin, PPSSPP, MAME, Cemu, Ryujinx, Xemu, Citra, Xenia, RPCS3.");
        if (resolved == null) return MessageWindow(game.Title, $"No mapping found for {game.Title}.");
        var log = Path.Combine(pluginDirectory, "padmaster.log");
        Window window = null!;
        void Pick(string id)
        {   // the dropdown is the pick: redraw with that pad and keep it as the setting for every game
            try
            {
                var store = StorePath(launchBoxRoot); (Settings.Read(store, _ => { }) with { Controller = id }).Write(store);  // ponytail: no Revision bump, so the assistant's placement logic ignores this write; a simultaneous bridge write could drop it, pick again
                var again = Resolve(game, emulator, pluginDirectory, launchBoxRoot, id) ?? throw new InvalidDataException($"No mapping found for {game.Title} on {id}.");
                window.Content = View(again); window.Title = game.Title + " — " + again.DisplayName;
                RotatingLog.Write(log, $"controls: {game.Title} -> {id} picked, {again.States.Count} states");
            }
            catch (Exception e) { RotatingLog.Write(log, $"controls: {game.Title} -> {id} error {e.Message}"); window.Content = new TextBlock { Text = e.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) }; }
        }
        MappingView View(Resolved r)
        {
            var (artwork, artworkError) = Publish(game, r, pluginDirectory, refresh);
            var view = new MappingView(r.DisplayName, r.SourcePath, r.States, artwork, artworkError, Controllers(pluginDirectory), r.ControllerId);
            view.ControllerPicked += Pick;
            void Open(Window dialog) { dialog.Owner = window; dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner; dialog.Show(); }
            view.AddControllerRequested += () => Open(OnboardWindow.Ask(pluginDirectory, launchBoxRoot, Pick));
            view.ChangeControllerRequested += () => Open(OnboardWindow.Change(r.ControllerId, r.DisplayName, pluginDirectory, launchBoxRoot, () => { Mapping.ClearRenders(pluginDirectory, r.ControllerId); Pick(r.ControllerId); }));
            view.RemoveControllerRequested += () =>
            {
                if (Onboard.Remove(r.ControllerId, pluginDirectory) is { } why) { window.Content = new TextBlock { Text = why, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) }; return; }
                var store = StorePath(launchBoxRoot); try { (Settings.Read(store, _ => { }) with { Controller = "" }).Write(store); } catch { }
                RotatingLog.Write(log, $"controls: controller {r.ControllerId} removed"); var next = Resolve(game, emulator, pluginDirectory, launchBoxRoot); if (next != null) { window.Content = View(next); window.Title = game.Title + " — " + next.DisplayName; }
            };
            view.RedoMapRequested += () =>
            {   // throw the map and its pictures away, then the same window asks "Generate one now?"
                try { File.Delete(Mapping.GamePath(pluginDirectory, game.Platform, game.Title)); Directory.Delete(Mapping.RenderDirectory(pluginDirectory, game.Platform, game.Title), true); } catch (IOException) { }
                PadMasterBadge.Generated.TryRemove(Mapping.GamePath(pluginDirectory, game.Platform, game.Title), out _); RotatingLog.Write(log, $"controls: {game.Title} map removed for redo");
                var again = Build(game, pluginDirectory, launchBoxRoot, findEmulator, refresh, offer: true); var content = again.Content; again.Content = null; window.Content = content; window.Title = again.Title;
            };
            return view;
        }
        window = new Window { Title = game.Title + " — " + resolved.DisplayName, Content = View(resolved), Width = Math.Min(1400, SystemParameters.WorkArea.Width * .95), Height = Math.Min(1000, SystemParameters.WorkArea.Height * .9), MinWidth = 480, MinHeight = 320 };
        AutomationProperties.SetAutomationId(window, "PadMappingWindow"); return window;
    }
}
