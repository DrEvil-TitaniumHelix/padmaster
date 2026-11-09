using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace PadMaster.Controls;

public sealed record Tile(string Caption, string Path, int Width, int Height);
public sealed record PlacedTile(Tile Tile, double Y, double Width, double Height);
public sealed record Band(double X, double Width, IReadOnlyList<PlacedTile> Tiles);

public sealed class PlayPanelPlugin : IGameLaunchingPlugin
{
    public const int MinBand = 400, CaptionHeight = 48, MaxStates = 6;
    const int GWL_EXSTYLE = -20, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, GA_ROOT = 2;
    public const uint RaiseFlags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW; public const nint HWND_TOPMOST = -1;
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(Pt point);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(nint hwnd, [Out] char[] text, int max);
    [StructLayout(LayoutKind.Sequential)] struct Pt { public int X, Y; }
    public static Func<int, int, nint> RootAt = (x, y) => GetAncestor(WindowFromPoint(new Pt { X = x, Y = y }), GA_ROOT);
    public static Func<nint, nint, uint, bool> SetPos = (hwnd, after, flags) => SetWindowPos(hwnd, after, 0, 0, 0, 0, flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hwnd, int command);
    public static Action<nint> Hide = hwnd => ShowWindow(hwnd, 0);
    public static Func<string> ForegroundTitle = () => TitleOf(GetForegroundWindow());
    public static string TitleOf(nint hwnd)
    {
        try { if (hwnd == 0) return "unknown"; var text = new char[256]; var n = GetWindowTextW(hwnd, text, text.Length); return n > 0 ? new string(text, 0, n) : "unknown"; }
        catch { return "unknown"; }
    }
    public static DispatcherTimer? Timer { get; private set; }
    [DllImport("user32.dll")] static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size, Left, Top, Right, Bottom, WorkLeft, WorkTop, WorkRight, WorkBottom; public uint Flags; }
    public static readonly List<Window> Open = new();
    static long generation;
    public static long Launch() => Interlocked.Increment(ref generation);
    public static long Exit() => Interlocked.Increment(ref generation);
    static string Dir => Path.GetDirectoryName(typeof(PlayPanelPlugin).Assembly.Location)!;
    static void Log(string dir, string text) => RotatingLog.Write(Path.Combine(dir, "padmaster.log"), "play panel: " + text);
    static void Post(Action action) { var d = Application.Current?.Dispatcher; if (d == null || d.CheckAccess()) action(); else d.BeginInvoke(action); }

    public sealed class Keeper
    {
        public static readonly TimeSpan CoveredAfter = TimeSpan.FromSeconds(5);
        readonly string dir; readonly (nint Hwnd, int X, int Y)[] bands; bool raised, coveredLogged, hidden; DateTime since, reported;
        public Keeper(string dir, IEnumerable<(nint Hwnd, int X, int Y)> bands) { this.dir = dir; this.bands = bands.ToArray(); }
        bool[] Ours() => bands.Select(b => RootAt(b.X, b.Y) == b.Hwnd).ToArray();
        public void Tick(DateTime now)
        {
            try
            {
                // LaunchBox tells plugins nothing about pausing; its pause window's title is the only signal. The panel must not sit on that menu.
                if (ForegroundTitle().Contains("Pause Screen", StringComparison.OrdinalIgnoreCase)) { if (!hidden) { hidden = true; foreach (var b in bands) Hide(b.Hwnd); Log(dir, "hidden for the pause screen"); } return; }
                if (hidden) { hidden = false; since = default; Log(dir, "back after the pause screen"); }  // RaiseFlags below shows the bands again
                if (since != default && now - since >= CoveredAfter && now - reported < CoveredAfter) return;
                var before = Ours(); foreach (var b in bands) SetPos(b.Hwnd, HWND_TOPMOST, RaiseFlags); var after = Ours();
                if (!after.Contains(false)) { since = default; if (!raised && before.Contains(false)) { raised = true; Log(dir, "re-asserted topmost over " + ForegroundTitle()); } return; }
                if (since == default) since = now;
                else if (now - since >= CoveredAfter) { if (!coveredLogged) { coveredLogged = true; Log(dir, $"covered by exclusive fullscreen ({ForegroundTitle()}); nothing can draw over it"); } reported = now; }
            }
            catch (Exception e) { Log(dir, "error " + e.Message); }
        }
    }
    public static void Watch(string dir, IEnumerable<(nint Hwnd, int X, int Y)> bands)
    {
        Timer?.Stop(); var keeper = new Keeper(dir, bands);
        Timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => keeper.Tick(DateTime.Now), Dispatcher.CurrentDispatcher);
    }

    public void OnBeforeGameLaunching(IGame? game, IAdditionalApplication? app, IEmulator? emulator) { }
    public void OnGameExited() { var dir = Dir; Exit(); try { Post(() => Close(dir)); } catch (Exception e) { Log(dir, "error " + e.Message); } }
    public void OnAfterGameLaunched(IGame game, IAdditionalApplication? app, IEmulator? emulator)
    {
        var dir = Dir; var launch = Launch();
        if (!ControlsPlugin.IsEnabled(dir)) return;
        Task.Run(() =>
        {
            try { var tiles = Prepare(game, emulator ?? PluginHelper.DataManager.GetEmulatorById(game.EmulatorId), dir, Path.GetFullPath(Path.Combine(dir, "..", "..")), () => Post(() => PluginHelper.LaunchBoxMainViewModel?.RefreshData())); if (tiles != null) Post(() => Show(tiles, dir, launch)); }
            catch (Exception e) { Log(dir, "error " + e.Message); }
        });
    }

    public static IReadOnlyList<Tile>? Prepare(IGame game, IEmulator? emulator, string pluginDirectory, string launchBoxRoot, Action refresh)
    {
        var resolved = ControlsPlugin.Resolve(game, emulator, pluginDirectory, launchBoxRoot);
        if (resolved == null) { Log(pluginDirectory, "no mapping for " + game.Title); return null; }
        ControlsPlugin.Publish(game, resolved, pluginDirectory, refresh);
        return Tiles(resolved.States);
    }

    public static IReadOnlyList<Tile> Tiles(IEnumerable<RenderedState> states) => states.Take(MaxStates).SelectMany(s => s.BackPath == null ? new[] { Measure(s.State.Name, s.FrontPath) } : new[] { Measure(s.State.Name, s.FrontPath), Measure(s.State.Name + " (back)", s.BackPath) }).ToArray();
    static Tile Measure(string caption, string path)
    {
        var header = new byte[24]; using (var file = File.OpenRead(path)) file.ReadExactly(header);
        return new(caption, path, (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16)), (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20)));
    }

    public static IReadOnlyList<Band> Layout(int screenW, int screenH, IReadOnlyList<Tile> tiles)
    {
        var band = (screenW - screenH * 16 / 9) / 2; if (band < MinBand || tiles.Count == 0) return [];
        var left = (tiles.Count + 1) / 2;
        Band Stack(double x, IEnumerable<Tile> own)
        {
            var list = own.ToArray(); var heights = list.Select(t => (double)band * t.Height / t.Width).ToArray();
            var scale = list.Length == 0 ? 1 : Math.Clamp((screenH - list.Length * CaptionHeight) / heights.Sum(), 0, 1);
            var placed = new List<PlacedTile>(); double y = 0;
            for (var i = 0; i < list.Length; i++) { var h = heights[i] * scale; placed.Add(new(list[i], y, band * scale, h)); y += CaptionHeight + h; }
            return new(x, band, placed);
        }
        return [Stack(0, tiles.Take(left)), Stack(screenW - band, tiles.Skip(left))];
    }

    public static long ExtendedStyle(Window window) => GetWindowLongPtrW(new WindowInteropHelper(window).EnsureHandle(), GWL_EXSTYLE);
    static nint NoActivate(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled) { if (msg != WM_MOUSEACTIVATE) return 0; handled = true; return MA_NOACTIVATE; }

    public static Window BandWindow(Band band, int screenLeft, int screenTop, int screenH, double dip)
    {
        var canvas = new Canvas { Width = band.Width, Height = screenH, Background = Brushes.Black, LayoutTransform = new ScaleTransform(dip, dip) };
        foreach (var placed in band.Tiles)
        {
            var caption = new TextBlock { Text = placed.Tile.Caption, Foreground = Brushes.White, FontSize = CaptionHeight * .55, Width = band.Width, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Canvas.SetTop(caption, placed.Y + CaptionHeight * .15); canvas.Children.Add(caption);
            var bitmap = new BitmapImage(); using (var file = File.OpenRead(placed.Tile.Path)) { bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = file; bitmap.EndInit(); } bitmap.Freeze();
            var image = new Image { Source = bitmap, Width = placed.Width, Height = placed.Height, Stretch = Stretch.Uniform };
            Canvas.SetLeft(image, (band.Width - placed.Width) / 2); Canvas.SetTop(image, placed.Y + CaptionHeight); canvas.Children.Add(image);
        }
        var window = new Window { Title = "Pad Master play panel", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true, Focusable = false, Background = Brushes.Black, Content = canvas, Left = (screenLeft + band.X) * dip, Top = screenTop * dip, Width = band.Width * dip, Height = screenH * dip };
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, GetWindowLongPtrW(hwnd, GWL_EXSTYLE) | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            HwndSource.FromHwnd(hwnd)?.AddHook(NoActivate);
        };
        return window;
    }

    public static void Show(IReadOnlyList<Tile> tiles, string dir, long launch)
    {
        try
        {
            if (launch != Interlocked.Read(ref generation)) { Log(dir, "skipped, game already exited"); return; }
            Close(dir, quiet: true);
            var main = Application.Current?.MainWindow; var hwnd = main == null ? 0 : new WindowInteropHelper(main).Handle;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() }; GetMonitorInfoW(MonitorFromWindow(hwnd, 1), ref info);
            int w = info.Right - info.Left, h = info.Bottom - info.Top;
            var bands = Layout(w, h, tiles);
            if (bands.Count == 0) { Log(dir, $"no side bands on this screen ({w}x{h})"); return; }
            var dip = main != null && PresentationSource.FromVisual(main) is { CompositionTarget: { } target } ? target.TransformFromDevice.M11 : 1;
            var shown = new List<(nint, int, int)>();
            foreach (var band in bands) { var window = BandWindow(band, info.Left, info.Top, h, dip); Open.Add(window); window.Show(); shown.Add((new WindowInteropHelper(window).Handle, info.Left + (int)(band.X + band.Width / 2), info.Top + h / 2)); }
            Watch(dir, shown);
            Log(dir, $"shown L={bands[0].Tiles.Count} R={bands[1].Tiles.Count} tiles on {w}x{h}");
        }
        catch (Exception e) { Log(dir, "error " + e.Message); }
    }

    public static void Close(string dir, bool quiet = false)
    {
        Timer?.Stop(); Timer = null;
        if (Open.Count == 0) return;
        foreach (var window in Open) { try { window.Close(); } catch { } }
        Open.Clear(); if (!quiet) Log(dir, "closed");
    }
}
