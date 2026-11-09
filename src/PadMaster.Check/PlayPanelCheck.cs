using System.IO;
using PadMaster.Controls;
using PadMaster.Readers;
using Unbroken.LaunchBox.Plugins.Data;

static class PlayPanelCheck
{
    const string Nes = "Nintendo Entertainment System", Ps2 = "Sony Playstation 2";
    static string Repo => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("play panel: " + message); }

    public static void Run()
    {
        Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "STA");
        ControlsPlugin.IsEnabled = _ => true; ControlsPlugin.StorePath = _ => Path.Combine(Path.GetTempPath(), "PadMaster-playpanel-settings", "settings.dat");   // never this PC's real store
        var wide =Enumerable.Range(0, 4).Select(i => new Tile("State " + i, "", 1600, 600)).ToArray();
        var bands = PlayPanelPlugin.Layout(5120, 1440, wide);
        Require(bands.Count == 2 && bands[0].X == 0 && bands[1].X == 3840 && bands.All(b => b.Width == 1280 && b.Tiles.Count == 2 && b.Tiles.All(t => t.Width == 1280 && t.Y + t.Height <= 1440)) && bands[0].Tiles[1].Y == PlayPanelPlugin.CaptionHeight + 480, "5120x1440: two 1280 bands, two full-width tiles each stacked inside the band");
        var tall = PlayPanelPlugin.Layout(5120, 1440, Enumerable.Range(0, 6).Select(i => new Tile("State " + i, "", 1182, 800)).ToArray());
        Require(tall.Count == 2 && tall.All(b => b.Tiles.Count == 3 && b.Tiles.All(t => t.Width < 1280 && Math.Abs(t.Width / t.Height - 1182.0 / 800) < .01) && b.Tiles[^1].Y + b.Tiles[^1].Height <= 1440), "six real-size tiles scale down to fit the band height, aspect kept");
        Require(PlayPanelPlugin.Layout(2560, 1440, wide).Count == 0 && PlayPanelPlugin.Layout(5120, 1440, []).Count == 0, "2560x1440 and no tiles: no bands");
        var one = PlayPanelPlugin.Layout(5120, 1440, wide.Take(1).ToArray()); Require(one.Count == 2 && one[0].Tiles.Count == 1 && one[1].Tiles.Count == 0, "single tile: left band only");

        var root = LaunchBoxDir.Value; var scratch = MappingCheck.Scratch(); MappingCheck.Copy(Path.Combine(Repo, "controllers"), Path.Combine(scratch, "controllers")); MappingCheck.Copy(Path.Combine(Repo, "games"), Path.Combine(scratch, "games"));
        Func<object?[], object?> never = _ => throw new Exception("unmapped game must not write artwork");
        var images = Path.Combine(scratch, "Images", Nes, ImageTypes.ArcadeControlsInformation); var refreshes = 0;
        var blaster = MappingCheck.Library(root, "Blaster Master", args => MappingCheck.Allocate(images, "Blaster Master", args));
        var tiles = PlayPanelPlugin.Prepare(blaster.Game, blaster.Emulator, scratch, root, () => refreshes++) ?? throw new InvalidOperationException("play panel: Blaster Master has no tiles");
        Require(tiles.Count == 2 && tiles.Select(t => t.Caption).SequenceEqual(["Inside vehicle", "Out of vehicle"]) && tiles.All(t => File.Exists(t.Path) && t.Width > 0 && t.Height > 0), "Blaster Master: two front tiles with measured PNG sizes");
        int Lines(string text) => File.ReadAllLines(Path.Combine(scratch, "padmaster.log")).Count(l => l.EndsWith(text, StringComparison.Ordinal));
        var art = Directory.GetFiles(images, "Blaster Master-*.png").Order(StringComparer.Ordinal).ToArray();
        Require(art.Length == 2 && art.Zip(tiles).All(p => Artwork.HasMarker(File.ReadAllBytes(p.First), Artwork.Marker("Blaster Master", "gulikit-kk3-max", p.Second.Caption))) && refreshes == 1 && Lines("artwork: Blaster Master -> 2 files") == 1, "launch path writes marked LB artwork via the API path, logs once, refreshes once");
        var hashes = art.ToDictionary(p => p, File.ReadAllBytes);
        Require(PlayPanelPlugin.Prepare(blaster.Game, blaster.Emulator, scratch, root, () => refreshes++)!.Count == 2 && Directory.GetFiles(images).Length == 2 && hashes.All(h => File.ReadAllBytes(h.Key).SequenceEqual(h.Value)) && refreshes == 1 && Lines("artwork: Blaster Master -> 2 files") == 1 && Lines(" files") == 1, "second launch: zero artwork writes, no log line, no refresh");
        Require(tiles.All(t => DrawCheck.BandPainted(t.Path, 0, .3f) && !DrawCheck.BandPainted(t.Path, .7f, 1)), "API returns no images: header text only, no logo");
        var unmapped = MappingCheck.Game("Nobody: Home?", blaster.Game.EmulatorId, blaster.Game.ApplicationPath, "", never); var sweepRefreshes = 0;
        Require(ArtworkSweepPlugin.Run([blaster.Game, unmapped], _ => blaster.Emulator, scratch, root, () => sweepRefreshes++) == (1, 0, 0) && sweepRefreshes == 0 && Directory.GetFiles(images).Length == 2, "sweep: mapped game only, unchanged art writes nothing and does not refresh");
        Require(ArtworkSweepPlugin.Run([blaster.Game], _ => null, scratch, root, () => sweepRefreshes++) == (1, 0, 1) && sweepRefreshes == 0, "sweep: no emulator counts as skipped, never throws");
        var badge = new PadMasterBadge(scratch); Require(badge.GetAppliesToGame(blaster.Game) && !badge.GetAppliesToGame(unmapped) && badge.UniqueId == "PadMasterMapped" && badge.DefaultIcon.Width == 64, "PM badge: mapped game only, 64 px icon");
        var states = ControlsPlugin.Resolve(blaster.Game, blaster.Emulator, scratch, root)!.States;
        Require(PlayPanelPlugin.Tiles(Enumerable.Range(0, 7).Select(i => states[0] with { State = states[0].State with { Name = "S" + i } })).Count == 6, "seven states capped at six");
        Require(PlayPanelPlugin.Tiles([states[0] with { BackPath = states[0].FrontPath }]).Select(t => t.Caption).SequenceEqual([states[0].State.Name, states[0].State.Name + " (back)"]), "back picture follows its state");
        var ps2 = RetroArch.FromXml(root, Ps2); string bond;
        if (ps2 == null || !File.Exists(ps2.Value.App) || !File.Exists(Pcsx2.IniPath(ps2.Value.App))) bond = "007 SKIP (no PCSX2)";
        else
        {
            var eon = MappingCheck.Library(root, "007: Everything or Nothing", args => MappingCheck.Allocate(Path.Combine(scratch, "Images", Ps2, ImageTypes.ArcadeControlsInformation), "007: Everything or Nothing", args), Ps2);
            var bondTiles = PlayPanelPlugin.Prepare(eon.Game, eon.Emulator, scratch, root, () => { }) ?? throw new InvalidOperationException("play panel: 007 has no tiles");
            Require(bondTiles.Count(t => !t.Caption.EndsWith(" (back)")) == 2 && bondTiles[0].Caption == "On foot" && bondTiles.All(t => File.Exists(t.Path)), "007 on PCSX2: On foot + Driving tiles");
            bond = $"007 -> {bondTiles.Count} tiles";
        }
        Require(PlayPanelPlugin.Prepare(MappingCheck.Game("Nobody: Home?", blaster.Game.EmulatorId, blaster.Game.ApplicationPath, "", never), blaster.Emulator, scratch, root, () => { }) == null, "unmapped game: null");
        Require(PlayPanelPlugin.Prepare(blaster.Game, null, scratch, root, () => { }) == null, "no emulator: null");
        var log = File.ReadAllLines(Path.Combine(scratch, "padmaster.log")).Select(l => l[20..]).ToArray();
        Require(log.Contains("play panel: no mapping for Nobody: Home?") && log.Contains("play panel: no mapping for Blaster Master") && log.Count(l => l.StartsWith("play panel: no mapping", StringComparison.Ordinal)) == 2, "exact no-mapping log lines only");

        var window = PlayPanelPlugin.BandWindow(PlayPanelPlugin.Layout(5120, 1440, tiles)[0], 0, 0, 1440, 1 / 1.5);
        var style = PlayPanelPlugin.ExtendedStyle(window);
        Require((style & 0x08000000) != 0 && (style & 0x8) != 0 && (style & 0x80) != 0 && !window.ShowInTaskbar && !window.ShowActivated && !window.IsVisible && window.Topmost && window.WindowStyle == System.Windows.WindowStyle.None, "NOACTIVATE|TOPMOST|TOOLWINDOW, no taskbar, never activated, never shown");
        Require(Math.Abs(window.Width - 1280 / 1.5) < .01 && Math.Abs(window.Height - 1440 / 1.5) < .01 && window.Left == 0, "band window in DIPs for 150% scale");
        window.Close(); new PlayPanelPlugin().OnGameExited();
        Require(PlayPanelPlugin.Open.Count == 0, "no panel window ever opened");
        var stale = PlayPanelPlugin.Launch(); PlayPanelPlugin.Exit(); PlayPanelPlugin.Show(tiles, scratch, stale);
        Require(PlayPanelPlugin.Open.Count == 0 && File.ReadAllLines(Path.Combine(scratch, "padmaster.log")).Count(l => l.EndsWith("play panel: skipped, game already exited", StringComparison.Ordinal)) == 1, "exit before Show: stale Show creates no window and logs skipped");
        var launchA = PlayPanelPlugin.Launch(); var launchB = PlayPanelPlugin.Launch(); var panelB = PlayPanelPlugin.BandWindow(PlayPanelPlugin.Layout(5120, 1440, tiles)[1], 0, 0, 1440, 1); PlayPanelPlugin.Open.Add(panelB);
        PlayPanelPlugin.Show(tiles, scratch, launchA);
        Require(PlayPanelPlugin.Open.Count == 1 && PlayPanelPlugin.Open[0] == panelB && launchB > launchA && File.ReadAllLines(Path.Combine(scratch, "padmaster.log")).Count(l => l.EndsWith("play panel: skipped, game already exited", StringComparison.Ordinal)) == 2, "launch A then B: A's late Show neither opens nor closes anything, only B's panel survives");
        PlayPanelPlugin.Close(scratch); Require(PlayPanelPlugin.Open.Count == 0 && File.ReadAllLines(Path.Combine(scratch, "padmaster.log")).Any(l => l.EndsWith("play panel: closed", StringComparison.Ordinal)), "close empties Open and logs closed");
        var (rootAt, setPos, foreground, hide) = (PlayPanelPlugin.RootAt, PlayPanelPlugin.SetPos, PlayPanelPlugin.ForegroundTitle, PlayPanelPlugin.Hide);
        try
        {
            var calls = new List<(nint Hwnd, nint After, uint Flags)>(); var covering = true; var raiseWorks = true; nint left = 0x11, right = 0x22;
            PlayPanelPlugin.RootAt = (x, y) => covering ? 0x999 : x < 2560 ? left : right;
            PlayPanelPlugin.SetPos = (hwnd, after, flags) => { calls.Add((hwnd, after, flags)); if (raiseWorks) covering = false; return true; };
            PlayPanelPlugin.ForegroundTitle = () => "PCSX2";
            var t0 = new DateTime(2026, 11, 7, 18, 0, 0); var keeper = new PlayPanelPlugin.Keeper(scratch, [(left, 320, 1080), (right, 4800, 1080)]);
            keeper.Tick(t0);
            Require(calls.Count == 2 && calls.Select(c => c.Hwnd).SequenceEqual([left, right]) && calls.All(c => c.After == PlayPanelPlugin.HWND_TOPMOST && c.Flags == 0x53 && (c.Flags & 0x10) != 0) && Lines("play panel: re-asserted topmost over PCSX2") == 1, "first tick: SetWindowPos TOPMOST NOMOVE|NOSIZE|NOACTIVATE|SHOWWINDOW on both bands, z-order change logged");
            keeper.Tick(t0.AddSeconds(1)); keeper.Tick(t0.AddSeconds(2));
            Require(calls.Count == 6 && Lines("play panel: re-asserted topmost over PCSX2") == 1, "later ticks keep re-asserting, line only once");
            raiseWorks = false; covering = true;
            for (var i = 3; i <= 7; i++) keeper.Tick(t0.AddSeconds(i));
            Require(calls.Count == 16 && Lines("nothing can draw over it") == 0, "covered under five seconds: no report");
            keeper.Tick(t0.AddSeconds(8));
            Require(calls.Count == 18 && Lines("play panel: covered by exclusive fullscreen (PCSX2); nothing can draw over it") == 1, "five seconds covered: reported once");
            keeper.Tick(t0.AddSeconds(9)); keeper.Tick(t0.AddSeconds(12));
            Require(calls.Count == 18, "after the report: retries wait five seconds");
            keeper.Tick(t0.AddSeconds(13)); keeper.Tick(t0.AddSeconds(18));
            Require(calls.Count == 22 && Lines("nothing can draw over it") == 1, "retry every five seconds, no second report");
            raiseWorks = true; keeper.Tick(t0.AddSeconds(23)); keeper.Tick(t0.AddSeconds(24));
            Require(calls.Count == 26 && !covering && Lines("re-asserted topmost over PCSX2") == 1 && Lines("nothing can draw over it") == 1, "game drops to borderless: back to one-second re-asserts, no new lines");
            var hidden = new List<nint>(); PlayPanelPlugin.Hide = hidden.Add; PlayPanelPlugin.ForegroundTitle = () => "LaunchBox Pause Screen"; var raises = calls.Count;
            keeper.Tick(t0.AddSeconds(24.2)); keeper.Tick(t0.AddSeconds(24.4));
            Require(hidden.SequenceEqual([left, right]) && calls.Count == raises && Lines("hidden for the pause screen") == 1, "pause screen: both bands hidden once, not raised over the menu");
            PlayPanelPlugin.ForegroundTitle = () => "PCSX2"; keeper.Tick(t0.AddSeconds(24.6));
            Require(calls.Count == raises + 2 && calls[^1].Flags == PlayPanelPlugin.RaiseFlags && Lines("back after the pause screen") == 1, "resume: bands shown again");
            PlayPanelPlugin.RootAt = (_, _) => throw new InvalidOperationException("vanished"); keeper.Tick(t0.AddSeconds(25));
            Require(Lines("play panel: error vanished") == 1 && PlayPanelPlugin.TitleOf(0) == "unknown" && PlayPanelPlugin.TitleOf(0x7ffffff1) == "unknown", "seam failure logged, never thrown; zero or vanished hwnd -> unknown");
            PlayPanelPlugin.Watch(scratch, [(left, 320, 1080)]);
            Require(PlayPanelPlugin.Timer is { IsEnabled: true, Interval.TotalSeconds: 1 }, "Show starts a one-second dispatcher timer");
            PlayPanelPlugin.Close(scratch); Require(PlayPanelPlugin.Timer == null && PlayPanelPlugin.Open.Count == 0, "Close stops and releases the timer");
        }
        finally { PlayPanelPlugin.Close(scratch); (PlayPanelPlugin.RootAt, PlayPanelPlugin.SetPos, PlayPanelPlugin.ForegroundTitle, PlayPanelPlugin.Hide) = (rootAt, setPos, foreground, hide); }
        Console.WriteLine("OK  play panel: 5120x1440 -> 2 bands x 2 tiles, 2560x1440 -> none, never activates");
        Console.WriteLine("OK  play panel: re-asserts topmost without activating; exclusive fullscreen reported once");
        Console.WriteLine($"INFO play panel: stale Show skipped, late A does not touch B, Blaster Master -> {tiles.Count} tiles + {art.Length} LB artwork files, {bond}, seven states -> 6, scratch {scratch}");
    }
}
